// 3DS PLC 시뮬레이터 — TODO(M2): docs/SPEC.md §6 그대로 구현.
// FluentModbus.ModbusTcpServer로 D0~D6 HR 노출(:1502), 배경 루프(50ms):
//  - C_Flag=1 감지 → C 읽고 즉시 C_*·C_Flag=0 → TiltDelay → [분류 시작: Ready=0 + TgtFloor=0 클리어]
//      → SortDuration → R_CellNo·R_Seq 쓰고 R_Flag=1, Ready=1
//  - TgtFloor!=0 && TgtFloor!=CurFloor → Ready=0(이동) → MoveDuration → CurFloor=TgtFloor (TgtFloor 유지!) → Ready=1
//  - 고장 주입(콘솔 키/설정): R_Seq 불일치, R_Flag 지연, 무응답(OFFLINE 유발)
//  - 모든 레지스터 변화 + WCS 쓰기 수신을 타임스탬프 로그로 출력(시나리오 검증용)
Console.WriteLine("Wcs.Sim3ds — M2에서 구현 (docs/SPEC.md §6)");
