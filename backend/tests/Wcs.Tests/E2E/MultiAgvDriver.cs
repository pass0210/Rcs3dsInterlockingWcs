using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Wcs.Tests.E2E;

// ════════════════════════════════════════════════════════════════════════════
// MultiAgvDriver — 다중 AGV RCS HTTP 클라이언트 드라이버 (계약 §3.1·§3.3)
//
//   AGV 1대 워크플로(IF-05 → IF-09 → IF-10)를 단일 사이클로 실행하고,
//   N대를 Barrier 동시 도달 + 독립 HttpClient로 동시 구동한다.
//   자동 스위트와 라이브 구동(§3.3)이 같은 드라이버 로직을 공유한다(중복 구현 금지).
//
//   드라이버는 HttpClient 팩토리만 의존 → 자동 스위트(WebApplicationFactory.CreateClient)와
//   라이브(new HttpClient{BaseAddress=...})가 동일 코드를 호출한다.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>AGV 1대의 IF-05~IF-10 한 사이클 입력(AGV별 고유 pId/agvNo/barcode 부여).</summary>
public sealed record AgvJob(
    int    PId,
    int    AgvNo,
    string Barcode,
    int    ChuteNo,        // IF-09/IF-10에 쓸 chuteNo(IF-05 OK면 응답값으로 덮어씀)
    int    InductionNo = 1,
    int    Qty         = 1,
    bool   DoArrival   = true,   // IF-09 도착 보고 수행 여부(I1/I2 순서 시나리오에서 끔)
    bool   DoDeposit   = true);  // IF-10 투입 보고 수행 여부

/// <summary>AGV 1대 사이클의 단계별 결과(테스트 단언·라이브 관찰용).</summary>
public sealed record AgvResult(
    int               PId,
    HttpStatusCode    If05Status,
    string?           If05Result,    // "OK" | "NG" | null(400)
    int?              ChuteNo,
    HttpStatusCode?   If09Status,
    HttpStatusCode?   If10Status,
    string?           If10Result);

/// <summary>
/// 다중 AGV 드라이버. HttpClient 생성 델리게이트만 받으므로 자동/라이브 양쪽에서 동일 구동.
/// </summary>
public sealed class MultiAgvDriver
{
    private readonly Func<HttpClient> _clientFactory;

    public MultiAgvDriver(Func<HttpClient> clientFactory)
    {
        _clientFactory = clientFactory;
    }

    /// <summary>WebApplicationFactory에서 드라이버 생성(자동 스위트).</summary>
    public static MultiAgvDriver ForFactory(WebApplicationFactory<global::Program> factory) =>
        new(factory.CreateClient);

    /// <summary>라이브 base URL에서 드라이버 생성(§3.3 — orchestrator 라이브 구동).</summary>
    public static MultiAgvDriver ForBaseUrl(string baseUrl) =>
        new(() => new HttpClient { BaseAddress = new Uri(baseUrl) });

    // ── AGV 1대 워크플로(IF-05 → IF-09 → IF-10) ───────────────────────────────
    /// <summary>
    /// AGV 1대의 한 사이클. 제공된 client로 IF-05→(IF-09)→(IF-10)을 순차 실행.
    /// IF-05 NG/400이면 이후 단계 생략(자연 흐름). 단계별 상태·결과를 AgvResult로 반환.
    /// </summary>
    public static async Task<AgvResult> RunOneAsync(HttpClient client, AgvJob job, CancellationToken ct = default)
    {
        // ── IF-05 목적지 조회 ──────────────────────────────────────────────────
        var if05Req = new
        {
            pId         = job.PId,
            agvNo       = job.AgvNo,
            barcode     = job.Barcode,
            inductionNo = job.InductionNo,
            qty         = job.Qty,
            timeStamp   = (string?)null,
        };
        var if05Resp = await client.PostAsJsonAsync("/api/v1/destination-query", if05Req, ct);

        if (if05Resp.StatusCode != HttpStatusCode.OK)
            return new AgvResult(job.PId, if05Resp.StatusCode, null, null, null, null, null);

        var if05Body = await if05Resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        string if05Result = if05Body.GetProperty("result").GetString()!;
        int?   chuteNo     = if05Body.TryGetProperty("chuteNo", out var cn) && cn.ValueKind == JsonValueKind.Number
                             ? cn.GetInt32() : null;

        if (if05Result != "OK")
            return new AgvResult(job.PId, HttpStatusCode.OK, if05Result, chuteNo, null, null, null);

        int useChuteNo = chuteNo ?? job.ChuteNo;

        // ── IF-09 도착 보고(운영층 정렬 트리거 — 소터면) ────────────────────────
        HttpStatusCode? if09Status = null;
        if (job.DoArrival)
        {
            var if09Req = new { pId = job.PId, chuteNo = useChuteNo, agvNo = job.AgvNo, timeStamp = (string?)null };
            var if09Resp = await client.PostAsJsonAsync("/api/v1/arrival-report", if09Req, ct);
            if09Status = if09Resp.StatusCode;
        }

        // ── IF-10 투입 보고(IF-11 셀 지정 트리거 — 소터면) ──────────────────────
        HttpStatusCode? if10Status = null;
        string?         if10Result = null;
        if (job.DoDeposit)
        {
            var if10Req = new { pId = job.PId, barcode = job.Barcode, chuteNo = useChuteNo, agvNo = job.AgvNo };
            var if10Resp = await client.PostAsJsonAsync("/api/v1/deposit-report", if10Req, ct);
            if10Status = if10Resp.StatusCode;
            if (if10Resp.StatusCode == HttpStatusCode.OK)
            {
                var if10Body = await if10Resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                if10Result = if10Body.GetProperty("result").GetString();
            }
        }

        return new AgvResult(job.PId, HttpStatusCode.OK, if05Result, chuteNo, if09Status, if10Status, if10Result);
    }

    // ── N대 동시 구동 (Barrier 동시 도달 — 진성 경합) ──────────────────────────
    /// <summary>
    /// N개 AGV 잡을 **동시에** 실행(Barrier로 IF-05 발사 시점을 정렬, AGV별 독립 HttpClient).
    /// 단일 idle 경로 함정 회피(계약 §6 ③) — 실 동시 HTTP로 경합을 일으킨다.
    /// </summary>
    public async Task<IReadOnlyList<AgvResult>> RunConcurrentAsync(
        IReadOnlyList<AgvJob> jobs, CancellationToken ct = default)
    {
        using var barrier = new Barrier(jobs.Count);
        var tasks = jobs.Select(job => Task.Run(async () =>
        {
            using var client = _clientFactory();
            // 모든 AGV가 동시에 IF-05를 때리도록 배리어 동기화(진성 경합).
            barrier.SignalAndWait(ct);
            return await RunOneAsync(client, job, ct);
        }, ct)).ToArray();

        return await Task.WhenAll(tasks);
    }

    /// <summary>잡 1개를 새 클라이언트로 실행(순차 시나리오·헬퍼).</summary>
    public async Task<AgvResult> RunSingleAsync(AgvJob job, CancellationToken ct = default)
    {
        using var client = _clientFactory();
        return await RunOneAsync(client, job, ct);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// E2EWait — 고정 sleep 금지(메타교훈). 조건/카운트 폴링 동기화 헬퍼.
//   전이당-1건 안정 카운트(WaitUntilExact)·스냅샷 폴링(WaitForSnapshot)으로 flaky 0.
// ════════════════════════════════════════════════════════════════════════════
public static class E2EWait
{
    /// <summary>condition()이 true가 될 때까지 폴링. 타임아웃이면 Assert.Fail.</summary>
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs, string msg, int pollMs = 25)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }

    /// <summary>async condition()이 true가 될 때까지 폴링(DB 조회 등).</summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, int timeoutMs, string msg, int pollMs = 30)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }

    /// <summary>count()가 expected를 stableCount회 연속 반환할 때까지 폴링(추가 전이 없음=안정).</summary>
    public static async Task UntilExactAsync(
        Func<int> countFunc, int expected, int stableCount, int timeoutMs, string msg, int pollMs = 30)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        int consecutive = 0;
        while (consecutive < stableCount)
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntilExact 타임아웃({timeoutMs}ms): {msg} (현재={countFunc()}, 기대={expected})");
            if (countFunc() == expected) consecutive++;
            else                         consecutive = 0;
            await Task.Delay(pollMs);
        }
    }

    /// <summary>async count() 버전.</summary>
    public static async Task UntilExactAsync(
        Func<Task<int>> countFunc, int expected, int stableCount, int timeoutMs, string msg, int pollMs = 40)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        int consecutive = 0;
        while (consecutive < stableCount)
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntilExact 타임아웃({timeoutMs}ms): {msg} (현재={await countFunc()}, 기대={expected})");
            if (await countFunc() == expected) consecutive++;
            else                               consecutive = 0;
            await Task.Delay(pollMs);
        }
    }
}
