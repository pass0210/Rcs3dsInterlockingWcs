using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Wcs.Data;

// ════════════════════════════════════════════════════════════════════════════
// WcsDbContext — ERD.md 16테이블 + provider 분기(SQL Server / SQLite)
//
// provider 분기 원칙 (ERD.md §인덱스):
//   SQL Server : filtered index (p_id) WHERE is_active=1 + rowversion 동시성 토큰
//   SQLite     : 일반 UNIQUE(p_id, is_active) + int 버전 컬럼(XminRowVersion)
//
// 연결문자열·provider 선택은 appsettings — 하드코딩 금지(절대규칙 8).
// ════════════════════════════════════════════════════════════════════════════

public class WcsDbContext : DbContext
{
    // ── 상수 — provider 이름은 문자열 비교로만 사용 ────────────────────────
    public const string ProviderSqlite    = "Microsoft.EntityFrameworkCore.Sqlite";
    public const string ProviderSqlServer = "Microsoft.EntityFrameworkCore.SqlServer";

    public WcsDbContext(DbContextOptions<WcsDbContext> options) : base(options) { }

    // ── DbSet (16 테이블) ──────────────────────────────────────────────────
    // 기준정보
    public DbSet<Destination>     Destinations     { get; set; } = null!;
    public DbSet<Cell>            Cells            { get; set; } = null!;
    public DbSet<CellAssignment>  CellAssignments  { get; set; } = null!;
    public DbSet<Agv>             Agvs             { get; set; } = null!;
    public DbSet<Printer>         Printers         { get; set; } = null!;
    public DbSet<ChuteDetail>     ChuteDetails     { get; set; } = null!;
    public DbSet<Induction>       Inductions       { get; set; } = null!;
    // 운영 축·오더
    public DbSet<WorkBatch>       WorkBatches      { get; set; } = null!;
    public DbSet<WcsOrder>        Orders           { get; set; } = null!;
    public DbSet<OrderItem>       OrderItems       { get; set; } = null!;
    // 실행·이력
    public DbSet<Piece>           Pieces           { get; set; } = null!;
    public DbSet<PieceEvent>      PieceEvents      { get; set; } = null!;
    public DbSet<SorterCommand>   SorterCommands   { get; set; } = null!;
    public DbSet<PlcEvent>        PlcEvents        { get; set; } = null!;
    public DbSet<Alarm>           Alarms           { get; set; } = null!;
    public DbSet<DestinationEvent> DestinationEvents { get; set; } = null!;
    // 횡단 운영 로그(S-OBSERVABILITY) — 17번째 테이블
    public DbSet<OperationLog>    OperationLogs    { get; set; } = null!;

    // ── B2B(작업 테스트 데이터) DbSet 6개 (S-B2B-1 — append) ────────────────
    // 기존 17테이블과 완전 분리. 형상: docs/B2B-SCHEMA.md §1·§2. 네임스페이스 Wcs.Data.B2B.
    public DbSet<B2B.TestData>    B2bTestData      { get; set; } = null!;
    public DbSet<B2B.TestLog>     B2bTestLogs      { get; set; } = null!;
    public DbSet<B2B.WorkResult>  B2bWorkResults   { get; set; } = null!;
    public DbSet<B2B.Box>         B2bBoxes         { get; set; } = null!;
    public DbSet<B2B.BoxItem>     B2bBoxItems      { get; set; } = null!;
    public DbSet<B2B.ApiCallLog>  B2bApiCallLogs   { get; set; } = null!;

    // ── 보조 프로퍼티 — 현재 provider 판별 ───────────────────────────────
    private bool IsSqlite    => Database.ProviderName == ProviderSqlite;
    private bool IsSqlServer => Database.ProviderName == ProviderSqlServer;

    protected override void OnModelCreating(ModelBuilder m)
    {
        base.OnModelCreating(m);
        ConfigureDestination(m);
        ConfigureCell(m);
        ConfigureCellAssignment(m);
        ConfigureAgv(m);
        ConfigurePrinter(m);
        ConfigureChuteDetail(m);
        ConfigureInduction(m);
        ConfigureWorkBatch(m);
        ConfigureWcsOrder(m);
        ConfigureOrderItem(m);
        ConfigurePiece(m);
        ConfigurePieceEvent(m);
        ConfigureSorterCommand(m);
        ConfigurePlcEvent(m);
        ConfigureAlarm(m);
        ConfigureDestinationEvent(m);
        ConfigureOperationLog(m);
        // ── B2B(작업 테스트 데이터) 6테이블 (S-B2B-1 — append) ─────────────
        ConfigureB2bTestData(m);
        ConfigureB2bTestLog(m);
        ConfigureB2bWorkResult(m);
        ConfigureB2bBox(m);
        ConfigureB2bBoxItem(m);
        ConfigureB2bApiCallLog(m);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 기준정보 테이블 설정
    // ────────────────────────────────────────────────────────────────────────

    private void ConfigureDestination(ModelBuilder m)
    {
        m.Entity<Destination>(e =>
        {
            e.ToTable("destination");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            e.Property(x => x.ChuteNo).IsRequired();
            e.HasIndex(x => x.ChuteNo).IsUnique().HasDatabaseName("UQ_destination_chute_no");

            // enum → string + CHECK
            e.Property(x => x.DestType)
             .HasConversion<string>()
             .HasMaxLength(20)
             .IsRequired();
            e.Property(x => x.Status)
             .HasConversion<string>()
             .HasMaxLength(20)
             .IsRequired();

            e.Property(x => x.Floor).IsRequired(false);
            e.Property(x => x.IsActive).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            // provider 분기: rowversion vs int 동시성 토큰
            ConfigureConcurrency(e, x => x.RowVersion, x => x.XminRowVersion);

            // 네비게이션 관계
            // S-SQLSERVER-FK-CASCADE: 필수 FK는 Restrict(NoAction)로 명시 — SQL Server 1785
            // (다중 캐스케이드 경로) 방지. 앱은 캐스케이드 삭제 미의존(append-only + 배치 퍼지)이라
            // 런타임 동작 영향 0. destination→{chute_detail,cell,destination_event}는 각각
            // sorter_command·cell_assignment 등으로 다시 수렴하므로 Cascade면 1785 유발.
            e.HasOne(x => x.ChuteDetail)
             .WithOne(x => x.Destination)
             .HasForeignKey<ChuteDetail>(x => x.DestinationId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Cells)
             .WithOne(x => x.Destination)
             .HasForeignKey(x => x.DestinationId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Events)
             .WithOne(x => x.Destination)
             .HasForeignKey(x => x.DestinationId)
             .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private void ConfigureCell(ModelBuilder m)
    {
        m.Entity<Cell>(e =>
        {
            e.ToTable("cell");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            e.Property(x => x.CellNo).IsRequired();
            // UQ(destination_id, cell_no)
            e.HasIndex(x => new { x.DestinationId, x.CellNo })
             .IsUnique()
             .HasDatabaseName("UQ_cell_destination_cell_no");

            e.Property(x => x.Capacity).IsRequired(false);
            e.Property(x => x.Enabled).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
        });
    }

    private void ConfigureCellAssignment(ModelBuilder m)
    {
        m.Entity<CellAssignment>(e =>
        {
            e.ToTable("cell_assignment");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            e.Property(x => x.AssignedAt).IsRequired();
            e.Property(x => x.ReleasedAt).IsRequired(false);
            e.Property(x => x.CreatedAt).IsRequired();

            // MINOR-4: (cell_id) WHERE released_at IS NULL 부분 유니크 — 동시 이중 셀 점유 방지
            if (IsSqlite)
            {
                e.HasIndex(x => x.CellId)
                 .IsUnique()
                 .HasFilter("\"ReleasedAt\" IS NULL")
                 .HasDatabaseName("UQ_cell_assignment_cell_active");
            }
            else
            {
                // SQL Server: 물리 컬럼명 PascalCase
                e.HasIndex(x => x.CellId)
                 .IsUnique()
                 .HasFilter("[ReleasedAt] IS NULL")
                 .HasDatabaseName("UQ_cell_assignment_cell_active");
            }

            // S-SQLSERVER-FK-CASCADE: cell_assignment는 cell·wcs_order 양쪽에서 수렴 →
            // Cascade면 다중 경로(1785). 필수 FK Restrict 명시.
            e.HasOne(x => x.Cell)
             .WithMany(x => x.Assignments)
             .HasForeignKey(x => x.CellId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Order)
             .WithMany(x => x.CellAssignments)
             .HasForeignKey(x => x.OrderId)
             .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureAgv(ModelBuilder m)
    {
        m.Entity<Agv>(e =>
        {
            e.ToTable("agv");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            e.Property(x => x.AgvNo).IsRequired();
            e.HasIndex(x => x.AgvNo).IsUnique().HasDatabaseName("UQ_agv_agv_no");

            e.Property(x => x.Floor).IsRequired();
            e.Property(x => x.Enabled).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
        });
    }

    private static void ConfigurePrinter(ModelBuilder m)
    {
        m.Entity<Printer>(e =>
        {
            e.ToTable("printer");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            e.Property(x => x.PrinterNo).IsRequired();
            e.HasIndex(x => x.PrinterNo).IsUnique().HasDatabaseName("UQ_printer_printer_no");

            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.ConnInfo).HasMaxLength(100).IsRequired(false);
            e.Property(x => x.Enabled).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
        });
    }

    private static void ConfigureChuteDetail(ModelBuilder m)
    {
        m.Entity<ChuteDetail>(e =>
        {
            e.ToTable("chute_detail");
            // PK = FK (1:1, CHUTE 전용)
            e.HasKey(x => x.DestinationId);
            e.Property(x => x.DestinationId).ValueGeneratedNever();

            e.Property(x => x.DefaultFullQty).IsRequired();
            e.Property(x => x.WorkFullQty).IsRequired();
            e.Property(x => x.PrinterId).IsRequired(false);
            e.Property(x => x.LastClearedAt).IsRequired(false);
            e.Property(x => x.Zone).HasMaxLength(50).IsRequired(false);
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            e.HasOne(x => x.Printer)
             .WithMany(x => x.ChuteDetails)
             .HasForeignKey(x => x.PrinterId)
             .IsRequired(false);
        });
    }

    private static void ConfigureInduction(ModelBuilder m)
    {
        m.Entity<Induction>(e =>
        {
            e.ToTable("induction");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            e.Property(x => x.InductionNo).IsRequired();
            e.HasIndex(x => x.InductionNo).IsUnique().HasDatabaseName("UQ_induction_induction_no");

            e.Property(x => x.Floor).IsRequired();
            e.Property(x => x.Enabled).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
        });
    }

    // ────────────────────────────────────────────────────────────────────────
    // 운영 축·오더 테이블 설정
    // ────────────────────────────────────────────────────────────────────────

    private void ConfigureWorkBatch(ModelBuilder m)
    {
        m.Entity<WorkBatch>(e =>
        {
            e.ToTable("work_batch");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            e.Property(x => x.WorkDate).IsRequired();
            e.Property(x => x.BatchNo).HasMaxLength(100).IsRequired();
            e.Property(x => x.WaveNo).IsRequired();
            // UQ(work_date, batch_no, wave_no)
            e.HasIndex(x => new { x.WorkDate, x.BatchNo, x.WaveNo })
             .IsUnique()
             .HasDatabaseName("UQ_work_batch_date_batch_wave");

            e.Property(x => x.Status)
             .HasConversion<string>()
             .HasMaxLength(20)
             .IsRequired();
            e.Property(x => x.OpenedAt).IsRequired(false);
            e.Property(x => x.ClosedAt).IsRequired(false);
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            ConfigureConcurrency(e, x => x.RowVersion, x => x.XminRowVersion);
        });
    }

    private void ConfigureWcsOrder(ModelBuilder m)
    {
        m.Entity<WcsOrder>(e =>
        {
            e.ToTable("wcs_order");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            e.Property(x => x.OrderNo).HasMaxLength(100).IsRequired();
            // UQ(work_batch_id, order_no)
            e.HasIndex(x => new { x.WorkBatchId, x.OrderNo })
             .IsUnique()
             .HasDatabaseName("UQ_wcs_order_batch_order_no");

            e.Property(x => x.OrderType)
             .HasConversion<string>()
             .HasMaxLength(20)
             .IsRequired();
            e.Property(x => x.RefNo).HasMaxLength(200).IsRequired(false);
            e.Property(x => x.RefName).HasMaxLength(200).IsRequired(false);
            e.Property(x => x.DestinationId).IsRequired(false);
            e.Property(x => x.DestAssignType)
             .HasConversion<string>()
             .HasMaxLength(20)
             .IsRequired(false);
            e.Property(x => x.DestAssignedAt).IsRequired(false);
            e.Property(x => x.Status)
             .HasConversion<string>()
             .HasMaxLength(20)
             .IsRequired();
            e.Property(x => x.StartedAt).IsRequired(false);
            e.Property(x => x.ClosedAt).IsRequired(false);
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            // 보조 인덱스: (work_batch_id, status)
            e.HasIndex(x => new { x.WorkBatchId, x.Status })
             .HasDatabaseName("IX_wcs_order_batch_status");

            ConfigureConcurrency(e, x => x.RowVersion, x => x.XminRowVersion);

            // S-SQLSERVER-FK-CASCADE: 필수 FK(work_batch) Restrict 명시. destination은 nullable
            // FK라 이미 비-Cascade(EF 기본) — 변경 불요.
            e.HasOne(x => x.WorkBatch)
             .WithMany(x => x.Orders)
             .HasForeignKey(x => x.WorkBatchId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Destination)
             .WithMany(x => x.Orders)
             .HasForeignKey(x => x.DestinationId)
             .IsRequired(false);
        });
    }

    private void ConfigureOrderItem(ModelBuilder m)
    {
        m.Entity<OrderItem>(e =>
        {
            e.ToTable("order_item");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            e.Property(x => x.Barcode).HasMaxLength(200).IsRequired();
            // UQ(order_id, barcode)
            e.HasIndex(x => new { x.OrderId, x.Barcode })
             .IsUnique()
             .HasDatabaseName("UQ_order_item_order_barcode");

            e.Property(x => x.PlannedQty).IsRequired();
            e.Property(x => x.ReservedQty).IsRequired();
            e.Property(x => x.SortedQty).IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            ConfigureConcurrency(e, x => x.RowVersion, x => x.XminRowVersion);

            // S-SQLSERVER-FK-CASCADE: 필수 FK Restrict 명시(piece가 order_item·destination 등에서
            // 다중 수렴 — Cascade면 1785).
            e.HasOne(x => x.Order)
             .WithMany(x => x.Items)
             .HasForeignKey(x => x.OrderId)
             .OnDelete(DeleteBehavior.Restrict);
        });
    }

    // ────────────────────────────────────────────────────────────────────────
    // 실행·이력 테이블 설정
    // ────────────────────────────────────────────────────────────────────────

    private void ConfigurePiece(ModelBuilder m)
    {
        m.Entity<Piece>(e =>
        {
            e.ToTable("piece");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            e.Property(x => x.PId).IsRequired();
            e.Property(x => x.IsActive).IsRequired();

            // MAJOR-1: 멱등 DB 백스톱 — 부분 유니크 인덱스 (p_id) WHERE is_active=1 AND status IN (...)
            // static _recordLock 대체 — DB 레벨 진성 멱등 보장.
            // ※ 교정: SQL Server filtered index 컬럼명은 물리 PascalCase(IsActive) 사용.
            if (IsSqlite)
            {
                // SQLite: expression index (EF HasFilter로 WHERE 조건 표현)
                // SQLite는 partial index에서 컬럼명 대소문자 구분 없음
                e.HasIndex(x => x.PId)
                 .IsUnique()
                 .HasFilter("\"IsActive\" = 1 AND \"Status\" IN ('DEPOSITED','CELL_ASSIGNED','LOADED')")
                 .HasDatabaseName("UQ_piece_pid_active_status");
            }
            else
            {
                // SQL Server: filtered unique index — 물리 컬럼명 PascalCase 사용(교정)
                e.HasIndex(x => x.PId)
                 .IsUnique()
                 .HasFilter("[IsActive] = 1 AND [Status] IN ('DEPOSITED','CELL_ASSIGNED','LOADED')")
                 .HasDatabaseName("UQ_piece_pid_active_status");
            }

            // 보조 인덱스
            e.HasIndex(x => x.Status)
             .HasDatabaseName("IX_piece_status");
            e.HasIndex(x => new { x.DestinationId, x.Status })
             .HasDatabaseName("IX_piece_dest_status");
            e.HasIndex(x => new { x.DestinationId, x.DepositedAt })
             .HasDatabaseName("IX_piece_dest_deposited");
            // piece 보조: (order_item_id)
            e.HasIndex(x => x.OrderItemId)
             .HasDatabaseName("IX_piece_order_item");

            e.Property(x => x.Barcode).HasMaxLength(200).IsRequired();
            e.Property(x => x.Qty).IsRequired();
            e.Property(x => x.DepositedAt).IsRequired(false);
            // MINOR-5: destination_id nullable FK (NG DENIED 시 NULL — 임의 fallback 제거)
            e.Property(x => x.DestinationId).IsRequired(false);
            e.Property(x => x.OrderItemId).IsRequired(false);
            e.Property(x => x.AgvId).IsRequired(false);
            e.Property(x => x.InductionId).IsRequired(false);
            e.Property(x => x.Status)
             .HasConversion<string>()
             .HasMaxLength(30)
             .IsRequired();
            e.Property(x => x.ClientTs).HasMaxLength(30).IsRequired(false);
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            ConfigureConcurrency(e, x => x.RowVersion, x => x.XminRowVersion);

            // MINOR-5: destination_id nullable FK
            e.HasOne(x => x.Destination)
             .WithMany(x => x.Pieces)
             .HasForeignKey(x => x.DestinationId)
             .IsRequired(false);
            e.HasOne(x => x.OrderItem)
             .WithMany(x => x.Pieces)
             .HasForeignKey(x => x.OrderItemId)
             .IsRequired(false);
            e.HasOne(x => x.Agv)
             .WithMany()
             .HasForeignKey(x => x.AgvId)
             .IsRequired(false);
            e.HasOne(x => x.Induction)
             .WithMany()
             .HasForeignKey(x => x.InductionId)
             .IsRequired(false);
        });
    }

    private static void ConfigurePieceEvent(ModelBuilder m)
    {
        m.Entity<PieceEvent>(e =>
        {
            e.ToTable("piece_event");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            e.Property(x => x.EventType)
             .HasConversion<string>()
             .HasMaxLength(20)
             .IsRequired();
            e.Property(x => x.Reason).HasMaxLength(100).IsRequired(false);
            e.Property(x => x.PayloadJson).IsRequired(false);
            e.Property(x => x.ClientTs).HasMaxLength(30).IsRequired(false);
            e.Property(x => x.At).IsRequired();

            // (at) 선두 인덱스 + (piece_id, at) 보조
            e.HasIndex(x => x.At).HasDatabaseName("IX_piece_event_at");
            e.HasIndex(x => new { x.PieceId, x.At }).HasDatabaseName("IX_piece_event_piece_at");

            // S-SQLSERVER-FK-CASCADE: 필수 FK Restrict 명시.
            e.HasOne(x => x.Piece)
             .WithMany(x => x.Events)
             .HasForeignKey(x => x.PieceId)
             .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureSorterCommand(ModelBuilder m)
    {
        m.Entity<SorterCommand>(e =>
        {
            e.ToTable("sorter_command");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            e.Property(x => x.CSeq).IsRequired();
            e.Property(x => x.CellNo).IsRequired();
            e.Property(x => x.CWrittenAt).IsRequired();
            e.Property(x => x.RSeq).IsRequired(false);
            e.Property(x => x.RCellNo).IsRequired(false);
            e.Property(x => x.RFlagAt).IsRequired(false);
            e.Property(x => x.Status)
             .HasConversion<string>()
             .HasMaxLength(20)
             .IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();

            // S-SQLSERVER-FK-CASCADE: sorter_command는 piece·cell 양쪽에서 수렴, 두 경로 모두
            // destination으로 거슬러 올라가 다중 캐스케이드 경로(1785, 대표 케이스
            // FK_sorter_command_piece_PieceId) 유발 → 필수 FK Restrict 명시.
            e.HasOne(x => x.Piece)
             .WithMany(x => x.Commands)
             .HasForeignKey(x => x.PieceId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Cell)
             .WithMany(x => x.Commands)
             .HasForeignKey(x => x.CellId)
             .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigurePlcEvent(ModelBuilder m)
    {
        m.Entity<PlcEvent>(e =>
        {
            e.ToTable("plc_event");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            e.Property(x => x.Kind)
             .HasConversion<string>()
             .HasMaxLength(20)
             .IsRequired();
            e.Property(x => x.Register).HasMaxLength(20).IsRequired();
            e.Property(x => x.OldVal).IsRequired(false);
            e.Property(x => x.NewVal).IsRequired(false);
            e.Property(x => x.At).IsRequired();

            // (at) 선두 인덱스
            e.HasIndex(x => x.At).HasDatabaseName("IX_plc_event_at");
        });
    }

    private static void ConfigureAlarm(ModelBuilder m)
    {
        m.Entity<Alarm>(e =>
        {
            e.ToTable("alarm");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            e.Property(x => x.Code).HasMaxLength(100).IsRequired();
            e.Property(x => x.Severity)
             .HasConversion<string>()
             .HasMaxLength(10)
             .IsRequired();
            e.Property(x => x.PieceId).IsRequired(false);
            e.Property(x => x.Message).IsRequired();
            e.Property(x => x.RaisedAt).IsRequired();
            e.Property(x => x.AckedAt).IsRequired(false);
            e.Property(x => x.CreatedAt).IsRequired();

            // (acked_at) WHERE acked_at IS NULL 부분 인덱스
            e.HasIndex(x => x.AckedAt).HasDatabaseName("IX_alarm_acked_at");

            e.HasOne(x => x.Piece)
             .WithMany(x => x.Alarms)
             .HasForeignKey(x => x.PieceId)
             .IsRequired(false);
        });
    }

    private static void ConfigureDestinationEvent(ModelBuilder m)
    {
        m.Entity<DestinationEvent>(e =>
        {
            e.ToTable("destination_event");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            e.Property(x => x.EventType)
             .HasConversion<string>()
             .HasMaxLength(30)
             .IsRequired();
            e.Property(x => x.DetailJson).IsRequired(false);
            e.Property(x => x.OperatorId).HasMaxLength(100).IsRequired(false);
            e.Property(x => x.At).IsRequired();

            // (destination_id, at) 인덱스
            e.HasIndex(x => new { x.DestinationId, x.At })
             .HasDatabaseName("IX_destination_event_dest_at");

            // S-SQLSERVER-FK-CASCADE: 필수 FK Restrict 명시.
            e.HasOne(x => x.Destination)
             .WithMany(x => x.Events)
             .HasForeignKey(x => x.DestinationId)
             .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureOperationLog(ModelBuilder m)
    {
        m.Entity<OperationLog>(e =>
        {
            e.ToTable("operation_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            // At — UTC 선두 인덱스(시계열 조회·퍼지). ERD 원칙 6.
            e.Property(x => x.At).IsRequired();
            e.HasIndex(x => x.At).HasDatabaseName("IX_operation_log_at");

            // enum → string + 길이(CHECK 대체). Serilog 레벨과 정합.
            e.Property(x => x.Category)
             .HasConversion<string>()
             .HasMaxLength(20)
             .IsRequired();
            e.Property(x => x.Action).HasMaxLength(40).IsRequired();
            e.Property(x => x.Level)
             .HasConversion<string>()
             .HasMaxLength(10)
             .IsRequired();

            // ── 스냅샷 식별 컬럼(FK 아님 — 1785 회피·이력 불변 원칙 5) ──────────────
            // operation_log는 어떤 마스터 테이블도 FK로 참조하지 않는다.
            // → 다중 캐스케이드 경로(SQL Server 1785) 원천 차단 + 마스터 변경에도 로그 불변.
            e.Property(x => x.SorterChuteNo).IsRequired(false);
            e.Property(x => x.DestinationId).IsRequired(false);
            e.Property(x => x.Barcode).HasMaxLength(200).IsRequired(false);
            e.Property(x => x.PId).IsRequired(false);

            // Detail — JSON nvarchar(max)(SQL Server) / TEXT(SQLite). 길이 미지정 = max.
            e.Property(x => x.Detail).IsRequired(false);

            // 보조 인덱스: (sorter_chute_no, at) — 특정 소터 시계열 조회.
            // filtered index 아님 → 물리 컬럼명 대소문자 207 함정 비해당.
            e.HasIndex(x => new { x.SorterChuteNo, x.At })
             .HasDatabaseName("IX_operation_log_sorter_at");
        });
    }

    // ────────────────────────────────────────────────────────────────────────
    // B2B(작업 테스트 데이터) 테이블 설정 (S-B2B-1)
    // 형상 정본: docs/B2B-SCHEMA.md §1. 컬럼명 원본 그대로 snake_case(HasColumnName).
    // 기존 17테이블 Configure 및 ModelSnapshot 기존 엔트리 무변경(add-only).
    // ⚠ created_at 은 B2B 로컬타임(DateTime.Now) — 원본 호환(B2C UTC와 상이 · 사용자 확정 Q3).
    // ────────────────────────────────────────────────────────────────────────

    private static void ConfigureB2bTestData(ModelBuilder m)
    {
        m.Entity<B2B.TestData>(e =>
        {
            e.ToTable("test_data");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();

            e.Property(x => x.BizDay).HasColumnName("biz_day").HasMaxLength(10).IsRequired();
            e.Property(x => x.Batch).HasColumnName("batch").HasMaxLength(10).IsRequired();
            e.Property(x => x.Barcode).HasColumnName("barcode").HasMaxLength(50).IsRequired();
            e.Property(x => x.ChuteNo).HasColumnName("chute_no").HasMaxLength(10).IsRequired();
            e.Property(x => x.ReceiveTime).HasColumnName("receive_time").IsRequired(false);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();  // B2B 로컬타임
            e.Property(x => x.Barcode2).HasColumnName("barcode2").HasMaxLength(50).IsRequired(false);

            e.HasIndex(x => new { x.BizDay, x.Batch }).HasDatabaseName("IX_test_data_biz_day_batch");
            e.HasIndex(x => x.Barcode).HasDatabaseName("IX_test_data_barcode");
            e.HasIndex(x => x.BizDay).HasDatabaseName("IX_test_data_biz_day");
        });
    }

    private static void ConfigureB2bTestLog(ModelBuilder m)
    {
        m.Entity<B2B.TestLog>(e =>
        {
            e.ToTable("test_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();

            e.Property(x => x.LogType).HasColumnName("log_type").HasMaxLength(10).IsRequired();
            e.Property(x => x.BizDay).HasColumnName("biz_day").HasMaxLength(10).IsRequired();
            e.Property(x => x.Batch).HasColumnName("batch").HasMaxLength(10).IsRequired();
            e.Property(x => x.Barcode).HasColumnName("barcode").HasMaxLength(50).IsRequired();
            e.Property(x => x.EquipmentNo).HasColumnName("equipment_no").HasMaxLength(20).IsRequired(false);
            e.Property(x => x.Pid).HasColumnName("pid").HasMaxLength(50).IsRequired(false);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(5).IsRequired(false);
            e.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(200).IsRequired(false);
            e.Property(x => x.LogTime).HasColumnName("log_time").IsRequired(false);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();  // B2B 로컬타임
            // test_data_id: FK 없이 인덱스만(원본 동일 · 1785 회피 · 이력 불변 · 사용자 확정 Q3).
            e.Property(x => x.TestDataId).HasColumnName("test_data_id").IsRequired(false);

            e.HasIndex(x => x.Barcode).HasDatabaseName("IX_test_log_barcode");
            e.HasIndex(x => x.LogType).HasDatabaseName("IX_test_log_log_type");
            e.HasIndex(x => x.LogTime).HasDatabaseName("IX_test_log_log_time");
            e.HasIndex(x => new { x.BizDay, x.LogType, x.LogTime })
             .HasDatabaseName("IX_test_log_biz_day_log_type_log_time");
            e.HasIndex(x => x.TestDataId).HasDatabaseName("IX_test_log_test_data_id");
        });
    }

    private static void ConfigureB2bWorkResult(ModelBuilder m)
    {
        m.Entity<B2B.WorkResult>(e =>
        {
            e.ToTable("work_result");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();

            e.Property(x => x.BizDay).HasColumnName("biz_day").HasMaxLength(10).IsRequired();
            e.Property(x => x.Batch).HasColumnName("batch").HasMaxLength(10).IsRequired();
            e.Property(x => x.Barcode).HasColumnName("barcode").HasMaxLength(50).IsRequired();
            e.Property(x => x.ChuteNo).HasColumnName("chute_no").HasMaxLength(20).IsRequired(false);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();  // B2B 로컬타임

            e.HasIndex(x => new { x.BizDay, x.Batch }).HasDatabaseName("IX_work_result_biz_day_batch");
        });
    }

    private static void ConfigureB2bBox(ModelBuilder m)
    {
        m.Entity<B2B.Box>(e =>
        {
            e.ToTable("box");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();

            e.Property(x => x.BizDay).HasColumnName("biz_day").HasMaxLength(10).IsRequired();
            e.Property(x => x.Batch).HasColumnName("batch").HasMaxLength(10).IsRequired();
            e.Property(x => x.BoxNo).HasColumnName("box_no").HasMaxLength(50).IsRequired();
            e.Property(x => x.ChuteNo).HasColumnName("chute_no").HasMaxLength(10).IsRequired();
            e.Property(x => x.EndTime).HasColumnName("end_time").HasMaxLength(50).IsRequired(false);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();  // B2B 로컬타임

            e.HasIndex(x => new { x.BizDay, x.Batch }).HasDatabaseName("IX_box_biz_day_batch");
            // 재전송 방지 UNIQUE (biz_day, batch, box_no)
            e.HasIndex(x => new { x.BizDay, x.Batch, x.BoxNo })
             .IsUnique()
             .HasDatabaseName("IX_box_biz_day_batch_box_no");
        });
    }

    private static void ConfigureB2bBoxItem(ModelBuilder m)
    {
        m.Entity<B2B.BoxItem>(e =>
        {
            e.ToTable("box_item");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();

            e.Property(x => x.BoxId).HasColumnName("box_id").IsRequired();
            e.Property(x => x.Barcode).HasColumnName("barcode").HasMaxLength(100).IsRequired();
            e.Property(x => x.Qty).HasColumnName("qty").IsRequired();

            e.HasIndex(x => x.BoxId).HasDatabaseName("IX_box_item_box_id");

            // 유일한 FK: box_item → box, ON DELETE CASCADE.
            // 단일 캐스케이드 경로 → SQL Server 1785 위험 없음(원본 동작 보존 · 사용자 확정 Q3).
            e.HasOne(x => x.Box)
             .WithMany(x => x.Items)
             .HasForeignKey(x => x.BoxId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureB2bApiCallLog(ModelBuilder m)
    {
        m.Entity<B2B.ApiCallLog>(e =>
        {
            e.ToTable("api_call_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();

            e.Property(x => x.Endpoint).HasColumnName("endpoint").HasMaxLength(100).IsRequired();
            e.Property(x => x.HttpMethod).HasColumnName("http_method").HasMaxLength(10).IsRequired();
            e.Property(x => x.RequestBody).HasColumnName("request_body").IsRequired(false);  // nvarchar(max)
            e.Property(x => x.ResponseStatus).HasColumnName("response_status").HasMaxLength(10).IsRequired(false);
            e.Property(x => x.ResponseBody).HasColumnName("response_body").IsRequired(false); // nvarchar(max)
            e.Property(x => x.HttpStatusCode).HasColumnName("http_status_code").IsRequired();
            e.Property(x => x.DurationMs).HasColumnName("duration_ms").IsRequired();
            e.Property(x => x.ClientIp).HasColumnName("client_ip").HasMaxLength(50).IsRequired(false);
            e.Property(x => x.ErrorMessage).HasColumnName("error_message").HasMaxLength(500).IsRequired(false);
            e.Property(x => x.CalledAt).HasColumnName("called_at").IsRequired();  // B2B 로컬타임

            e.HasIndex(x => x.CalledAt).HasDatabaseName("IX_api_call_log_called_at");
            e.HasIndex(x => x.Endpoint).HasDatabaseName("IX_api_call_log_endpoint");
        });
    }

    // ────────────────────────────────────────────────────────────────────────
    // 공통 헬퍼 — provider 분기 동시성 토큰 설정
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// MINOR-2: provider 분기 — 활성 provider 동시성 토큰 설정, 비활성 provider 컬럼 Ignore.
    /// - SQLite: XminRowVersion IsConcurrencyToken, RowVersion Ignore(물리 컬럼 미생성).
    /// - SQL Server: RowVersion IsRowVersion, XminRowVersion Ignore(물리 컬럼 미생성).
    /// Ignore(propertyName)으로 이중 물리 컬럼 제거 — 비활성 provider 컬럼은 스키마에 생성되지 않음.
    /// </summary>
    private void ConfigureConcurrency<T>(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e,
        System.Linq.Expressions.Expression<Func<T, byte[]?>> rowVersionExpr,
        System.Linq.Expressions.Expression<Func<T, int>> xminExpr)
        where T : class
    {
        // expression에서 프로퍼티명 추출 (Ignore 오버로드가 string을 요구)
        var rowVersionName = ((System.Reflection.MemberInfo)
            ((System.Linq.Expressions.MemberExpression)rowVersionExpr.Body).Member).Name;
        var xminName = ((System.Reflection.MemberInfo)
            ((System.Linq.Expressions.MemberExpression)xminExpr.Body).Member).Name;

        if (IsSqlite)
        {
            // SQLite: int 동시성 토큰 활성화, byte[]? RowVersion 컬럼은 물리 제거
            e.Property(xminExpr).IsConcurrencyToken();
            e.Ignore(rowVersionName);   // MINOR-2: 비활성 provider 컬럼 물리 제거
        }
        else
        {
            // SQL Server: rowversion 활성화, int XminRowVersion 컬럼은 물리 제거
            e.Property(rowVersionExpr)
             .IsRowVersion()
             .IsRequired(false);
            e.Ignore(xminName);         // MINOR-2: 비활성 provider 컬럼 물리 제거
        }
    }
}
