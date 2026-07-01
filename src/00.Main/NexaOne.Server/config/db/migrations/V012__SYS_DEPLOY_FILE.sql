-- SYS Module: 배포 파일 업로드 / 클라이언트 자동 업데이트 (설계서 20.11)
-- 현행: 서버 공유 폴더에 배포 패키지 업로드 → 클라이언트 기동 시 버전 비교 후 자동 업데이트.
-- 웹 적응: 바이너리는 API 서버 디스크(IDeployFileStorage), 메타데이터만 본 테이블에 기록한다.
-- VERSION은 System.Version 형식 문자열 — 최신 버전 선정은 SQL 문자열 정렬이 아니라
-- 서비스 계층의 Version 비교로 한다 ('1.10.0' > '1.9.0' 보장).
CREATE TABLE SYS_DEPLOY_FILE (
    FILE_ID         NVARCHAR(32)    NOT NULL,   -- GUID("N") 저장 키 (스토리지 파일명과 동일)
    VERSION         NVARCHAR(20)    NOT NULL,   -- 예: 3.5.1.0
    FILE_NAME       NVARCHAR(260)   NOT NULL,   -- 업로드 원본 파일명 (다운로드 응답에 사용)
    HASH            NVARCHAR(64)    NOT NULL,   -- SHA-256 hex — 클라이언트 무결성 검증용
    FILE_SIZE       BIGINT          NOT NULL,
    DESCRIPTION     NVARCHAR(500)   NOT NULL,   -- 릴리스 노트
    FORCE_UPDATE    BIT             NOT NULL,   -- 강제 업데이트 여부 (클라이언트가 건너뛰기 불가)
    IS_ACTIVE       BIT             NOT NULL,   -- 문제 버전 회수: 비활성은 latest 선정/다운로드 제외
    UPLOADED_BY     NVARCHAR(50)    NOT NULL,
    UPLOADED_AT     DATETIME2       NOT NULL,
    CONSTRAINT PK_SYS_DEPLOY_FILE PRIMARY KEY (FILE_ID),
    CONSTRAINT UQ_SYS_DEPLOY_FILE_VERSION UNIQUE (VERSION)
);

CREATE INDEX IX_SYS_DEPLOY_FILE_ACTIVE ON SYS_DEPLOY_FILE (IS_ACTIVE, UPLOADED_AT DESC);
