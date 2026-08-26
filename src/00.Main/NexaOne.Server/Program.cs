using NexaOne.Server;

var builder = WebApplication.CreateBuilder(args);

// 실행 프로젝트는 조립만 담당한다. 프레임워크 공통 기능과 MES 기능의 세부 등록은
// 각 확장 메서드에 숨겨 다른 호스트에서도 동일한 구성을 재사용할 수 있게 한다.
builder.Services.AddNexaOneMes(builder.Configuration);

var app = builder.Build();

app.UseNexaOneMes();

// MES 모듈 초기화와 종료는 Generic Host 수명주기에 연결되어 있으므로
// 실행 진입점에서는 표준 웹 호스트 실행만 시작하면 된다.
await app.RunNexaOneMesAsync();

// WebApplicationFactory가 통합 테스트용 호스트 진입점을 찾을 수 있도록 공개한다.
public partial class Program { }
