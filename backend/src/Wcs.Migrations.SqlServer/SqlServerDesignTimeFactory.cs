using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Wcs.Data;

namespace Wcs.Migrations.SqlServer;

// ════════════════════════════════════════════════════════════════════════════
// SQL Server 마이그레이션 전용 design-time factory.
// 이 어셈블리(Wcs.Migrations.SqlServer)가 SQL Server 마이그레이션의 단독 홈.
//
// 마이그레이션 생성 명령:
//   dotnet ef migrations add Initial \
//     --project src/Wcs.Migrations.SqlServer \
//     --startup-project src/Wcs.Data
//
// 런타임 배선: UseSqlServer(..., sql => sql.MigrationsAssembly("Wcs.Migrations.SqlServer"))
// ════════════════════════════════════════════════════════════════════════════

/// <summary>SQL Server provider design-time factory — 독립 ModelSnapshot 보장.</summary>
public sealed class SqlServerDesignTimeFactory : IDesignTimeDbContextFactory<WcsDbContext>
{
    public WcsDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<WcsDbContext>();
        // design-time 전용 — 운영 연결문자열은 appsettings에서 주입(절대규칙 8)
        builder.UseSqlServer(
            "Server=(localdb)\\mssqllocaldb;Database=WcsDev;Trusted_Connection=True;",
            sql => sql.MigrationsAssembly("Wcs.Migrations.SqlServer")
                      .MigrationsHistoryTable("__EFMigrationHistory"));
        return new WcsDbContext(builder.Options);
    }
}
