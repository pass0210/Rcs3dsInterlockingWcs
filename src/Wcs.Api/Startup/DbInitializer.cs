using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wcs.Data;

namespace Wcs.Api.Startup;

// ════════════════════════════════════════════════════════════════════════════
// DbInitializer — 콜드스타트 자동 프로비저닝 (M5-P1).
//
// 실 호스트(dotnet run / Windows Service)가 빈/구버전 DB에서 기동할 때
// 스키마를 자동 적용(Migrate)하고, 개발/빈-DB 한정으로 기준정보를 시드한다.
// 직전 E2E에서 발견된 크래시(빈 wcs.db 기동 시 ChuteCapacityService가
// `no such table: chute_detail`로 죽음)를 정식 해소한다.
//
// 호출 위치: app.Build() 이후 app.Run() 이전 (IHostedService가 시작되기 전).
//   → ChuteCapacityService.StartAsync / SorterRegistryFactory.StartAsync가
//     DB를 조회하기 전에 스키마가 보장되어야 한다.
//
// ⚠ 테스트 안전 게이트 (최우선 제약):
//   5개 테스트 팩토리는 named in-memory SQLite(`Mode=Memory;Cache=Shared`)에
//   직접 EnsureCreated() + DbSeeder.Seed()로 스키마·시드를 주입한다.
//   이 경로에서 Migrate가 실행되면 (a) EnsureCreated가 만든 스키마에
//   __EFMigrationHistory 부재로 Migrate가 충돌하거나 (b) 시드가 중복 삽입된다.
//   → DbContext의 실제 연결이 in-memory SQLite이면 Migrate·시드를 모두 건너뛴다.
//     이 판별은 DI에 등록된 DbContext(테스트가 교체한 것)를 직접 검사하므로
//     테스트 배선을 변경하지 않고도 테스트 호스트에서 no-op이 된다.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 콜드스타트 DB 프로비저닝 — 스키마 자동 Migrate + dev/빈-DB 한정 시드.
/// 실 호스트 startup(app.Build 이후 app.Run 이전)에서 1회 호출.
/// 테스트 호스트(in-memory SQLite)에서는 자동으로 no-op.
/// </summary>
public static class DbInitializer
{
    // ── appsettings 게이트 키 (하드코딩 금지 — 절대규칙 #7, appsettings에서 외부화) ──
    /// <summary>Database:MigrateOnStartup — 콜드스타트 자동 Migrate 활성화. 기본 true.</summary>
    public const string MigrateOnStartupKey = "Database:MigrateOnStartup";

    /// <summary>Database:SeedOnStartup — 콜드스타트 dev 시드 활성화. 기본 false(운영 안전).</summary>
    public const string SeedOnStartupKey = "Database:SeedOnStartup";

    /// <summary>
    /// 콜드스타트 프로비저닝 실행. 실 호스트 startup에서만 호출.
    /// </summary>
    /// <param name="app">빌드된 WebApplication.</param>
    public static async Task ProvisionAsync(WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>()
                     .CreateLogger("Wcs.Api.Startup.DbInitializer");

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();

        // ── 테스트 안전 게이트: in-memory SQLite면 전부 건너뛴다 ────────────────
        // (테스트 팩토리가 EnsureCreated+DbSeeder.Seed로 이미 주입 — Migrate/시드 금지)
        if (IsInMemorySqlite(db))
        {
            log.LogInformation(
                "[DbInitializer] in-memory SQLite 감지 — 콜드스타트 Migrate·시드 건너뜀(테스트 호스트 보호).");
            return;
        }

        var config = app.Configuration;

        // ── ① 자동 Migrate (기본 on) ──────────────────────────────────────────
        // provider 분기는 AddDbContext 등록(Program.cs)에서 이미 결정됨
        // (Sqlite → Wcs.Migrations.Sqlite / SqlServer → Wcs.Migrations.SqlServer).
        // Database.MigrateAsync()는 등록된 provider의 마이그레이션 어셈블리를 적용한다.
        var migrateOnStartup = config.GetValue(MigrateOnStartupKey, defaultValue: true);
        if (migrateOnStartup)
        {
            log.LogInformation("[DbInitializer] 콜드스타트 자동 Migrate 시작 (provider={Provider}).",
                db.Database.ProviderName);
            await db.Database.MigrateAsync().ConfigureAwait(false);
            log.LogInformation("[DbInitializer] Migrate 완료 — 스키마 보장됨.");
        }
        else
        {
            log.LogInformation(
                "[DbInitializer] {Key}=false — 자동 Migrate 건너뜀(운영자 수동 마이그레이션 가정).",
                MigrateOnStartupKey);
        }

        // ── ② dev/빈-DB 한정 시드 (운영 안전 게이트) ───────────────────────────
        // 운영(production)은 실제 마스터데이터를 쓰므로 테스트 시드 자동 삽입 금지.
        // 게이트: SeedOnStartup=true(명시) 또는 Development 환경에서만 on. 기본 off.
        var seedOnStartup = config.GetValue<bool?>(SeedOnStartupKey);
        var seedEnabled = seedOnStartup ?? app.Environment.IsDevelopment();
        if (seedEnabled)
        {
            // 시드는 멱등(DbSeeder.Seed가 존재 행을 스킵). 그래도 운영 안전을 위해
            // 게이트 통과 시에만 호출한다.
            var agvFloorMap = config.GetSection("Floors:AgvNoToFloor")
                                    .Get<Dictionary<string, int>>();
            DbSeeder.Seed(db, agvFloorMap);
            log.LogInformation(
                "[DbInitializer] dev 시드 적용됨 (트리거: {Trigger}).",
                seedOnStartup.HasValue ? $"{SeedOnStartupKey}=true" : "Development 환경");
        }
        else
        {
            log.LogInformation(
                "[DbInitializer] 시드 게이트 off(운영 안전) — 빈 스키마만 프로비저닝. " +
                "dev 시드가 필요하면 {Key}=true 또는 ASPNETCORE_ENVIRONMENT=Development.",
                SeedOnStartupKey);
        }
    }

    // ── in-memory SQLite 판별 ─────────────────────────────────────────────────
    // 테스트 팩토리는 `Data Source=...;Mode=Memory;Cache=Shared`를 사용한다.
    // 실 호스트는 파일(`Data Source=wcs.db`) 또는 SqlServer를 사용한다.
    // SqliteConnectionStringBuilder.Mode == Memory 또는 DataSource == ":memory:"이면 in-memory.
    private static bool IsInMemorySqlite(WcsDbContext db)
    {
        if (!db.Database.IsSqlite()) return false;

        var conn = db.Database.GetDbConnection();
        try
        {
            var sb = new SqliteConnectionStringBuilder(conn.ConnectionString);
            return sb.Mode == SqliteOpenMode.Memory
                || string.Equals(sb.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 연결문자열 파싱 실패 시 안전쪽(in-memory로 간주하지 않음 — 실 호스트는 Migrate 필요).
            return false;
        }
    }
}
