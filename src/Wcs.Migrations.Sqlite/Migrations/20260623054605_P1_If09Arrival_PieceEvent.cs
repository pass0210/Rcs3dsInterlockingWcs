using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wcs.Migrations.Sqlite.Migrations
{
    /// <summary>
    /// RCS↔WCS 재설계 Phase 1 — PieceEventType에 IF09_ARRIVAL 추가.
    /// piece_event.event_type은 enum→string(TEXT, maxLength) 매핑이며 별도 CHECK 제약이 없으므로
    /// 새 enum 값 추가는 스키마 변경을 유발하지 않는다(Up/Down 본문 비어 있음 = 정상).
    /// 본 마이그레이션은 변경 시점을 이력에 남기고 provider별 ModelSnapshot을 동기화한다
    /// (P1 교훈: provider별 별도 스냅샷 유지). has-pending-model-changes = "No changes".
    /// </summary>
    public partial class P1_If09Arrival_PieceEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 의도적으로 비어 있음 — enum 값 추가는 event_type 컬럼 정의를 바꾸지 않음(CHECK 제약 없음).
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 의도적으로 비어 있음 — 위 Up과 대칭.
        }
    }
}
