# NexaMES 영구 release 브랜치 정책

## 브랜치 계약

- 개발 기준 브랜치는 `main`이고 릴리즈 용도는 영구 `release` 브랜치 하나만 사용합니다.
- `release/final`, `release/candidate`, `release/<version>`처럼 버전이나 단계를 브랜치 이름으로 만들지 않습니다.
- 버전별 배포물은 `release/<major>.<minor>.<patch>/` 디렉터리에 보존합니다. 디렉터리에는
  NexaMES 서버/모듈 DLL, 필요한 설정·정적 파일, ZIP과 SHA-256 manifest만 둡니다.
- `main`, `release`, 운영 version tag는 force push나 재지정을 하지 않습니다. 현재 feature
  브랜치는 검증·리뷰용이며 승인 전에는 `release`를 갱신하지 않습니다.

## 릴리즈 순서

1. 최신 `main`을 fast-forward로 검증하고 `dotnet build --warnaserror`, Unit/Server/boot,
   Portal 테스트·빌드·audit, SQLite 증분 검증을 통과시킵니다.
2. 복원한 SQL Server에서 V001부터 마지막 migration까지 `mssql-contract`와 rollback/lock
   근거를 확인합니다. 실 MSSQL 증거가 없으면 릴리즈 산출물을 만들지 않습니다.
3. `tools/ops/Test-Publish.ps1`로 게시 결과를 격리 폴더에서 단독 부팅하고 `/health`, 로그인,
   모듈 DLL closure를 확인합니다. 산출물의 각 DLL·ZIP은 크기와 SHA-256을 manifest에 기록합니다.
4. 승인된 하나의 artifact commit만 `release`에 반영하고 `release → main` PR을 no-ff 병합합니다.
   병합 후 `release`를 같은 main merge commit까지 fast-forward합니다.
5. 운영 tag `v<version>`은 정확한 main merge commit에 annotated tag로 만들고, 같은 manifest와
   DLL closure를 다시 검증한 뒤에만 GitHub Release/배포 저장소에 게시합니다.

## 미완료 상태의 안전 규칙

- GitHub Actions billing/spending-limit, private submodule credential, 실 MSSQL, 실제 설비
  Controller/HIL 중 하나라도 실패하거나 실행되지 않으면 PR은 draft/검토 대기로 유지합니다.
- 실제 Controller·라이선스·RID 증거가 없는 Motion/IO/Serial/Vision/SECS-GEM DLL을 NexaMES
  릴리즈에 포함하거나 성공으로 표시하지 않습니다. 해당 드라이버는 제품 Plugin/host가
  승인된 계약으로 조립할 때만 추가합니다.
- 이미 게시한 version 디렉터리·manifest·DLL은 덮어쓰지 않습니다. 수정은 새 patch 버전으로
  만들고 이전 tag와 artifact의 도달 가능성을 보존합니다.

이 정책은 Cleaner와 NexaFramework의 release 정책과 동일한 브랜치 topology를 사용하지만,
MES의 서버/모듈 DLL closure와 SQL Server 계약 게이트를 추가로 요구합니다.
