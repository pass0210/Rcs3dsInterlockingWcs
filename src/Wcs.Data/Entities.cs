namespace Wcs.Data;

// TODO(M4): docs/ERD.md의 16개 테이블을 EF Core 엔티티+DbContext로 구현.
// 원칙: 대리키 bigint / p_id 필터드 유니크(is_active) / 상태 enum HasConversion<string>() /
//       이력 테이블 append-only / row_version 동시성 / SQLite 개발 분기(ERD.md '인덱스' 참조).
public sealed class Placeholder { }
