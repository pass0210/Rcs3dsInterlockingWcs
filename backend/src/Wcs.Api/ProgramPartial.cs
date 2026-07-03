// Program 클래스를 public partial로 노출 — WebApplicationFactory 통합 테스트 접근용 (Scope F)
// top-level statements가 생성하는 Program 클래스를 외부에서 참조 가능하게 만든다.
// Microsoft.AspNetCore.Mvc.Testing의 WebApplicationFactory<Program>이 이 선언에 의존.
// 최상위 문(top-level statements)과 partial class 선언은 같은 파일에 공존 불가(CS8803).
// 따라서 별도 파일로 분리.
public partial class Program { }
