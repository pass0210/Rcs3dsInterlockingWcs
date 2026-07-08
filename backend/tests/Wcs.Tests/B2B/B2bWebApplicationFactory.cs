using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api;
using Wcs.Data;
using Xunit;

namespace Wcs.Tests.B2B;

// ════════════════════════════════════════════════════════════════════════════
// B2bWebApplicationFactory — B2B API 통합 테스트 전용 호스트.
//
// ⚠ 기존 FakeModbusWebApplicationFactory 는 _dbName 이 static — 인스턴스 2개가 같은 in-memory DB 를
//   공유한다. B2B 테스트가 별도 IClassFixture 로 두 번째 인스턴스를 만들면 double EnsureCreated/Seed 충돌.
//   → 무접촉 위해 기존 팩토리를 건드리지 않고, INSTANCE-level 고유 DB 명을 쓰는 전용 팩토리를 신설.
//
// B2B 는 PLC 무관 — B2C 시드 없이 스키마만 EnsureCreated(0 SORTER_3D → 소터 0대·Modbus 미연결,
//   0 CHUTE → ChuteCapacity 빈 집계). test_data 는 각 테스트가 scope 로 직접 시드.
// Provider=Sqlite 는 host setting(UseSetting)으로 Program 의 즉시평가 전에 주입(lessons 2026-06-30).
// ════════════════════════════════════════════════════════════════════════════
public sealed class B2bWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dbName = $"B2bTest_{Guid.NewGuid():N}";   // INSTANCE-level(핵심 — 인스턴스별 격리)
    private readonly SqliteConnection _anchor;

    public B2bWebApplicationFactory()
    {
        _anchor = new SqliteConnection($"Data Source={_dbName};Mode=Memory;Cache=Shared");
        _anchor.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:Provider", "Sqlite");

        builder.ConfigureServices(services =>
        {
            // WcsDbContext 를 named in-memory SQLite 로 교체(provider 결선만).
            var dbDescriptors = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<WcsDbContext>)
                         || d.ServiceType == typeof(WcsDbContext))
                .ToList();
            foreach (var d in dbDescriptors)
                services.Remove(d);

            var connStr = $"Data Source={_dbName};Mode=Memory;Cache=Shared";
            services.AddDbContext<WcsDbContext>(opts =>
                opts.UseSqlite(connStr, sqlite => sqlite.CommandTimeout(30))
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)),
                ServiceLifetime.Scoped);

            // 스키마만 생성(B2C 시드 없음). 앵커 연결로 EnsureCreated → B2B 6테이블 + 기존 17테이블 스키마.
            var dbOpts = new DbContextOptionsBuilder<WcsDbContext>()
                .UseSqlite(_anchor)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            using var db = new WcsDbContext(dbOpts);
            db.Database.EnsureCreated();
        });
    }

    // ── 비동기 종료(teardown 데드락 회피 — 기존 팩토리와 동일 패턴) ─────────────────
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => DisposeAsyncCore().AsTask();
    public override ValueTask DisposeAsync() => DisposeAsyncCore();

    private async ValueTask DisposeAsyncCore()
    {
        // api_call_log 큐 완료 → 백그라운드 writer 결정적 종료(testhost-channel-race 방어).
        try { Services.GetService<Wcs.Api.B2B.ApiCallLogQueue>()?.Complete(); }
        catch { /* 종료 경쟁 — 무시 */ }
        await base.DisposeAsync().ConfigureAwait(false);
        _anchor.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _anchor.Dispose();
    }
}
