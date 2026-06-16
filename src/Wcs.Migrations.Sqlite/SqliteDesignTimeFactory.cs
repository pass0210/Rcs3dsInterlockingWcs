using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Wcs.Data;

namespace Wcs.Migrations.Sqlite;

// ════════════════════════════════════════════════════════════════════════════
// SQLite 마이그레이션 전용 design-time factory.
// 이 어셈블리(Wcs.Migrations.Sqlite)가 SQLite 마이그레이션의 단독 홈.
//
// 마이그레이션 생성 명령:
//   dotnet ef migrations add Initial \
//     --project src/Wcs.Migrations.Sqlite \
//     --startup-project src/Wcs.Data
//
// 런타임 배선: UseSqlite(..., sqlite => sqlite.MigrationsAssembly("Wcs.Migrations.Sqlite"))
// ════════════════════════════════════════════════════════════════════════════

/// <summary>SQLite provider design-time factory — 독립 ModelSnapshot 보장.</summary>
public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<WcsDbContext>
{
    public WcsDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<WcsDbContext>();
        // design-time 전용 — 운영 연결문자열은 appsettings에서 주입(절대규칙 8)
        builder.UseSqlite(
            "Data Source=wcs_dev.db",
            sqlite => sqlite.MigrationsAssembly("Wcs.Migrations.Sqlite")
                            .MigrationsHistoryTable("__EFMigrationHistory"));
        return new WcsDbContext(builder.Options);
    }
}
